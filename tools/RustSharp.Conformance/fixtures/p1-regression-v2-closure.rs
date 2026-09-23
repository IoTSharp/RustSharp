fn apply() -> i32 {
    let add = |value: i32| value + 1;
    add(3)
}

fn main() {
    println!("{}", apply());
}
