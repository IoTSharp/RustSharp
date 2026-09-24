const BASE: i32 = 6 * 7;

enum Reading {
    Empty,
    Sample(i32),
    Pair { left: i32, right: i32 },
}

fn score(value: Reading) -> i32 {
    match value {
        Reading::Empty => 0,
        Reading::Sample(value) => value,
        Reading::Pair { left, right } => left + right,
    }
}

fn nested(value: &&i32) -> i32 { **value }
fn packed(value: (&i32, i32)) -> &i32 { value.0 }
fn tail(values: &mut [i32], start: usize) -> &mut [i32] { &mut values[start..] }
fn identity(values: &[i32]) -> &[i32] { values }
fn promoted() -> &'static i32 { &42 }

fn selected_pattern(value: (i32, i32)) -> i32 {
    match value {
        (value, _) | (_, value) if value > 1 => value,
        _ => 0,
    }
}

fn sample_or_default(value: Reading) -> i32 {
    let Reading::Sample(number) = value else { return 19; };
    number
}

fn slice_pattern(values: &[i32]) -> i32 {
    match values {
        [] => 0,
        [only] => *only,
        [first, middle @ .., last] => *first + middle.len() as i32 + *last,
    }
}

fn bump_ends(values: &mut [i32]) {
    match values {
        [first, middle @ .., last] if middle.len() > 0 => {
            *first += 1;
            middle[0] += 2;
            *last += 3;
        },
        _ => (),
    };
}

fn main() {
    println!("{}", BASE);
    println!("{}", score(Reading::Empty));
    println!("{}", score(Reading::Sample(7)));
    println!("{}", score(Reading::Pair { right: 5, left: 3 }));
    let owner = 17;
    let reference = &owner;
    let indirect = &reference;
    println!("{}", nested(indirect));
    println!("{}", *packed((reference, 1)));
    let mut values = [1, 2, 3, 4];
    let view: &mut [i32] = &mut values;
    let selected = tail(view, 1);
    selected[1] = BASE;
    println!("{}", selected.len());
    println!("{}", selected[1]);
    println!("{}", values[2]);
    let short = [9];
    let left: &[i32] = &short;
    let right: &[i32] = &values;
    let choose = false;
    let joined = if choose { left } else { right };
    println!("{}", identity(joined).len());
    let middle = &right[1..=2];
    println!("{}", middle[1]);
    println!("{}", *promoted());
    println!("{}", const { 2 * 5 });
    let mut total = 2;
    let mut accumulate = |amount: i32| { total += amount; };
    accumulate(3);
    accumulate(4);
    println!("{}", total);
    println!("{}", (3 << 2) | 1);
    println!("{}", (true ^ false) as i32);
    println!("{}", selected_pattern((0, 8)));
    println!("{}", sample_or_default(Reading::Empty));
    println!("{}", sample_or_default(Reading::Sample(11)));
    let (first, .., last) = (1, 2, 3, 4);
    println!("{}", first + last);
    let mut pair = (2, 3);
    let (ref mut first, ref last) = pair;
    *first += *last;
    println!("{}", pair.0);
    println!("{}", slice_pattern(&values));
    let mut rest_values = [1, 2, 3, 4];
    let [_, rest @ ..] = &mut rest_values;
    rest[0] = 8;
    let copied = *rest;
    println!("{}", copied[0]);
    *rest = [5, 6, 7];
    println!("{}", rest_values[1] + rest_values[3]);
    let mut ends = [1, 2, 3, 4];
    bump_ends(&mut ends);
    println!("{}", ends[0] + ends[1] + ends[3]);
}
