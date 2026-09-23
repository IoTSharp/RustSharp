fn invalid() -> i32 {
    let add = |value: i32| value + 1;
    let escaped = add;
    0
}

fn main() {
    println!("{}", invalid());
}
