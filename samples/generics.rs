trait Marker {}

impl Marker for i32 {}
impl Marker for bool {}

struct Container<T> {
    value: T,
}

impl<T: Marker> Marker for Container<T> {}

fn identity<T>(value: T) -> T {
    value
}

fn marked<T: Marker>(value: T) -> T {
    identity(value)
}

fn choose<T>(flag: bool, left: T, right: T) -> T {
    if flag { left } else { right }
}

fn pair<T, U>(left: T, right: U) -> (T, U) {
    (left, right)
}

fn preserve<T>(value: Container<T>) -> Container<T> {
    value
}

fn main() {
    let answer: i32 = marked(identity::<i32>(42));
    let selected: i32 = choose(true, answer, 0);
    let flag: bool = marked(true);
    let result: (i32, bool) = pair(selected, flag);
    let number = marked(preserve(Container { value: result.0 }));
    let truth = marked(preserve(Container { value: result.1 }));
    println!("{}", number.value);
    println!("{}", truth.value);
}
